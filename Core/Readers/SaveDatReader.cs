﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GothicSaveEditor.Core.Primitives;
using GothicSaveEditor.Models;
using static GothicSaveEditor.Core.Readers.SavedatConstants;

namespace GothicSaveEditor.Core.Readers
{

    /// <summary>
    /// Структура сейва (друг за другом):
    /// 1. Заголовок, инфа о сейве
    /// 2. Диалоги
    /// 3. MIS (дневник)
    /// 4. Обычные переменные
    /// 5. Мусор
    /// </summary>
    public static class SaveDatReader
    {
        private static bool _dialogBegin;
        private static bool _dialogEnd;
        private static bool _dialog;
        private static bool _mission;

        public static List<GothicVariable> Read(string path)
        {
            var byteArray = ReadSaveBytes(path);
            var controlPoints = FindControlPoints(byteArray);
            
            _dialogBegin = false;
            _dialogEnd = false;
            _dialog = true;
            _mission = false;
            
            return ParseSaveGame(byteArray, controlPoints.Item1, controlPoints.Item2);
        }

        /// <summary>
        /// Читает сейвгейм и возвращает массив байтов
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static byte[] ReadSaveBytes(string path)
        {
            byte[] byteArray;
            try
            {
                byteArray = File.ReadAllBytes(path); // Читаем сейв побайтово
            }
            catch (Exception ex)
            {
                throw new Exception("SRReadBytesError", ex); // Ошибка чтения байтов файла
            }

            return byteArray;
        }

        /// <summary>
        /// Находит точку входа - где начинаются переменные, и точку выхода - где заканчиваются переменные.
        /// </summary>
        /// <returns></returns>
        private static Tuple<int, int> FindControlPoints(byte[] bytes)
        {
            var index = 0; // Итератор цикла
            // Скипаем ненужную bytes в начале сейва.
            try
            {
                for (; index < bytes.Length; index++)
                {
                    if (bytes[index] == 0x02
                        && bytes[index + 1] == 0x00
                        && bytes[index + 2] == 0x00
                        && bytes[index + 3] == 0x00
                        && bytes[index + 4] == 0x01
                        && bytes[index + 5] == 0x00
                        && bytes[index + 6] == 0x00
                        && bytes[index + 7] == 0x00)
                    {
                        index += 8;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("SRHeaderError", ex); // Заголовок неверный, сейв сломан
            }

            int maxByte;
            try
            {
                maxByte = BitConverter.ToInt32(bytes, index); // Последний байт, до которого следует читать (далее идет мусор)
            }
            catch (Exception ex)
            {
                throw new Exception("SRMaxByteError", ex); // Ошибка чтения номера последнего байта, сейв сломан.
            }
            if (maxByte == 0)
                throw new Exception("SRMaxByteError");
            index += 4;

            return new Tuple<int, int>(index, maxByte);
        }


        private static GothicVar ReadVariable(ref byte[] bytes, ref int rIndex)
        {
            var values = new int[1];
            var positions = new int[1];

            var arraySize = int.MaxValue;
            var arrIteration = -1;
            while (bytes[rIndex] == ValueMarker) // Начало значения переменной
            {
                rIndex += 5;
                if (bytes[rIndex] == NameMarker)
                {
                    break; // Если 0x01 то это текст, значит мы должны закончить считывание этой переменной
                }

                rIndex++;
                if (bytes[rIndex - 1] == NormalVarMarker) // Значение начинается здесь
                {
                    if (_dialogBegin)
                    {
                        _mission = true;
                        _dialog = false;
                        _dialogEnd = true;
                        _dialogBegin = false;
                    }

                    positions[0] = rIndex;
                    values[0] = BitConverter.ToInt32(bytes, rIndex);
                    if (arrIteration == -1)
                    {
                        arraySize = values[0];
                    }

                    if (arrIteration >= 0 && arrIteration < arraySize) // массив, предыдущие данные нам не нужны т.к это была длина массива.
                    {
                        if (arrIteration == 0)
                        {
                            positions = new int[arraySize];
                            values = new int[arraySize];
                        }

                        positions[arrIteration] = rIndex;
                        values[arrIteration] = BitConverter.ToInt32(bytes, rIndex);
                    }
                    arrIteration++;
                }
                else if (bytes[rIndex - 1] == DialogMarker) // Начало диалога
                {
                    values[0] = BitConverter.ToInt32(bytes, rIndex);
                    positions[0] = rIndex;
                }
                rIndex += 4; // Integer занимает 4 байта

                // ReSharper disable once InvertIf
                if (_dialog && bytes[rIndex - 5] == DialogMarker)
                {
                    _dialogBegin = true;
                    return new GothicVar(positions, values);
                }
            }
            return new GothicVar(positions, values);
        }

        
        private static string ReadName(ref byte[] bytes, ref int index)
        {
            int length = BitConverter.ToInt16(bytes, ++index);
            index += 2;
            if (length <= 0)
                return "";
            
            var sb = new StringBuilder(2048);
            var readTo = index + length;
            for (; index < readTo; index++)
            {
                sb.Append((char)bytes[index]);
            }
            index--;

            var str = sb.ToString();
            if (_mission == false)
            {
                return str;
            }

            if (str == "[]")
                _mission = false;

            return "";
        }

        private static List<GothicVariable> ParseSaveGame(byte[] byteArray, int startIndex, int lastIndex)
        {
            var needToReadValue = false;
            var varname = ""; // Название текущей переменной
            var gothicVar = new GothicVar();

            var variablesList = new List<GothicVariable>();

            // Начинаем парсить переменные!
            for (var index = startIndex; index < lastIndex; index++)
            {
                if (byteArray[index] == ValueMarker)
                {
                    if (_dialog) // В диалоге сначала идет чтение значения и только потом строка
                    {
                        gothicVar = ReadVariable(ref byteArray, ref index); // Передача идет по ссылке
                    }
                    else
                    {
                        // ReSharper disable once InvertIf
                        if (needToReadValue && varname.Trim().Length > 0) // Уже считали название переменной, считываем ее значение
                        {
                            gothicVar = ReadVariable(ref byteArray, ref index); // Читаем значение
                            if (_dialogEnd) // Если достигнут конец, переменная не будет сохранена, поскольку последний слайд получает неверное значение
                            {
                                _dialogEnd = false;
                            }
                            else
                            {
                                variablesList.AddRange(gothicVar.Values.Select((t, i) => gothicVar.Values.Length > 1
                                    ? new GothicVariable(varname, gothicVar.Positions[i], gothicVar.Values[0], i)
                                    : new GothicVariable(varname, gothicVar.Positions[i], gothicVar.Values[0])));
                            }
                            index--; //one back because of the while conditional i ++
                            needToReadValue = false; // Новая переменная строка должна читаться, пока не будет прочитано значение
                        }
                    }
                }
                else if (byteArray[index] == NameMarker)
                {
                    varname = ReadName(ref byteArray, ref index);
                    if (varname.Trim().Length > 0)
                    {
                        if (_dialog && _dialogBegin) // В диалоговом режиме центрирование переменной заканчивается, поэтому переменная генерируется здесь
                        {
                            variablesList.AddRange(gothicVar.Values.Select((t, i) => gothicVar.Values.Length > 1
                                ? new GothicVariable(varname, gothicVar.Positions[i], gothicVar.Values[0], i)
                                : new GothicVariable(varname, gothicVar.Positions[i], gothicVar.Values[0])));
                        }
                        else
                        {
                            needToReadValue = true; // Название было считано, теперь нужно считать значение
                        }
                    }
                }
            }
            if (variablesList.Count == 0)
            {
                throw new Exception("SREmptyVariablesList");
            }

            variablesList.Sort(new VariablesComparer());
            return variablesList;
        }
    }
}
