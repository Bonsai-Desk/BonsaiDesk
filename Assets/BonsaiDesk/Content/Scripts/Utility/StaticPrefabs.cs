﻿using UnityEngine;

public class StaticPrefabs : MonoBehaviour
{
    public static StaticPrefabs instance;

    public GameObject blockObjectPrefab;

    // Start is called before the first frame update
    private void Start()
    {
        instance = this;
    }
}